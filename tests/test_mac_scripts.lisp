;;; SPDX-License-Identifier: GPL-3.0-or-later
;;; Copyright (C) 2026 hrmcngs

;;; Run with: sbcl --script tests/test_mac_scripts.lisp
;;; Exercise routing with fake tools; never touch the installed input method.
(require :asdf)

(defparameter *root*
  (uiop:pathname-parent-directory-pathname
   (uiop:pathname-directory-pathname *load-truename*)))

(defun write-file (path text)
  (ensure-directories-exist path)
  (with-open-file (stream path :direction :output :if-exists :supersede)
    (write-string text stream)))

(defun fake-tool (work name body)
  (let ((path (merge-pathnames name work)))
    (write-file path (format nil "#!/bin/bash~%~A~%" body))
    (uiop:run-program (list "chmod" "+x" (namestring path)))))

(defun run-script (work name argument)
  (uiop:run-program
   (append (list "env"
                 (concatenate 'string "PATH=" (namestring work) ":" (uiop:getenv "PATH"))
                 (concatenate 'string "MELTYPE_TEST_LOG="
                              (namestring (merge-pathnames "calls" work)))
                 (concatenate 'string "MELTYPE_START_LOG_DIR=" (namestring work))
                 "bash" (namestring (merge-pathnames name work)))
           (if (listp argument) argument (list argument)))
   :output *standard-output* :error-output *error-output*))

(defun call-with-work-directory (test)
  (let ((work (uiop:ensure-directory-pathname
               (string-trim '(#\Newline #\Return)
                            (uiop:run-program
                             '("mktemp" "-d" "/tmp/meltype-lisp-tests.XXXXXX")
                             :output :string)))))
    (unwind-protect (funcall test work)
      (uiop:delete-directory-tree work :validate t))))

(defun test-build-only (work)
  (write-file (merge-pathnames "build-cli.sh" work)
              (uiop:read-file-string (merge-pathnames "mac/build-cli.sh" *root*)))
  (fake-tool work "build.sh" "echo \"$*\" >> \"$MELTYPE_TEST_LOG\"")
  (dolist (name '("dotnet" "swift")) (fake-tool work name "exit 0"))
  (run-script work "build-cli.sh" "--build")
  (assert (equal '("--no-install")
                 (uiop:read-file-lines (merge-pathnames "calls" work)))))

(defun test-update (work)
  (let* ((source (uiop:read-file-string (merge-pathnames "mac/update.sh" *root*)))
         (original "$HOME/Library/Input Methods/Meltype.app")
         (position (search original source))
         (app (namestring (merge-pathnames "Meltype.app" work))))
    (assert position)
    (write-file (merge-pathnames "update.sh" work)
                (concatenate 'string (subseq source 0 position) app
                             (subseq source (+ position (length original))))))
  (fake-tool work "Meltype.app/Contents/MacOS/Meltype"
             "echo \"app:$*\" >> \"$MELTYPE_TEST_LOG\"")
  (fake-tool work "build-cli.sh"
             "echo \"build:$MELTYPE_SKIP_START:$*\" >> \"$MELTYPE_TEST_LOG\"")
  (fake-tool work "uname" "echo Darwin")
  (fake-tool work "start-input-method.sh"
             "echo app: >> \"$MELTYPE_TEST_LOG\"")
  (fake-tool work "swift" "echo select >> \"$MELTYPE_TEST_LOG\"")
  (fake-tool work "open" "echo UNEXPECTED_OPEN >> \"$MELTYPE_TEST_LOG\"; exit 1")
  (run-script work "update.sh" nil)
  (let ((lines (uiop:read-file-lines (merge-pathnames "calls" work))))
    (assert (equal "build:1:--test --install" (first lines)))
    (assert (uiop:string-prefix-p "app:--check-inputs " (second lines)))
    (dolist (expected '("app:" "app:--register-input-source" "select"))
      (assert (member expected lines :test #'equal)))
    (assert (not (member "UNEXPECTED_OPEN" lines :test #'equal)))))

(defun test-launchd-start (work)
  (write-file (merge-pathnames "start-input-method.sh" work)
              (uiop:read-file-string (merge-pathnames "mac/start-input-method.sh" *root*)))
  (fake-tool work "launchctl"
             (format nil "echo \"$*\" >> \"$MELTYPE_TEST_LOG\"~%case \"$1\" in~% list) echo '\"PID\" = 123; Meltype_Connection' ;;~%esac"))
  (run-script work "start-input-method.sh" "/tmp/Meltype.app")
  (let ((lines (uiop:read-file-lines (merge-pathnames "calls" work))))
    (assert (equal "remove io.github.yksr-melt.Meltype.manual" (first lines)))
    (assert (uiop:string-prefix-p "submit -l io.github.yksr-melt.Meltype.manual -o " (second lines)))
    (assert (search "Meltype.stdout.log -e " (second lines)))
    (assert (search "Meltype.stderr.log -- /tmp/Meltype.app/Contents/MacOS/Meltype" (second lines)))
    (assert (equal "list io.github.yksr-melt.Meltype.manual" (third lines)))))

(defun test-update-failure-recovery (work)
  (test-update work)
  (dolist (failure '("build" "check"))
    (write-file (merge-pathnames "calls" work) "")
    (fake-tool work "build-cli.sh" (if (equal failure "build") "exit 23" "exit 0"))
    (fake-tool work "Meltype.app/Contents/MacOS/Meltype"
               (if (equal failure "check")
                   (format nil "if [[ \"$1\" == --check-inputs ]]; then exit 23; fi~%echo \"app:$*\" >> \"$MELTYPE_TEST_LOG\"")
                   "echo \"app:$*\" >> \"$MELTYPE_TEST_LOG\""))
    (let ((status nil))
      (handler-case (run-script work "update.sh" nil)
        (uiop:subprocess-error (condition)
          (setf status (uiop:subprocess-error-code condition))))
      (assert (eql 23 status)))
    (let ((lines (uiop:read-file-lines (merge-pathnames "calls" work))))
      (dolist (expected '("app:" "app:--register-input-source" "select"))
        (assert (member expected lines :test #'equal))))))

(defun test-launchd-retry (work)
  (test-launchd-start work)
  (write-file (merge-pathnames "calls" work) "")
  (fake-tool work "sleep" "exit 0")
  (fake-tool work "launchctl"
             (format nil "echo \"$*\" >> \"$MELTYPE_TEST_LOG\"~%case \"$1\" in~%submit) n=$(cat \"$MELTYPE_START_LOG_DIR/attempts\" 2>/dev/null || echo 0); echo $((n+1)) > \"$MELTYPE_START_LOG_DIR/attempts\" ;;~%list) n=$(cat \"$MELTYPE_START_LOG_DIR/attempts\"); if [[ $n -ge 2 ]]; then echo '\"PID\" = 123; Meltype_Connection'; fi ;;~%esac"))
  (run-script work "start-input-method.sh" "/tmp/Meltype.app")
  (assert (= 2 (parse-integer (uiop:read-file-string (merge-pathnames "attempts" work)))))
  (write-file (merge-pathnames "attempts" work) "0")
  (fake-tool work "launchctl"
             (format nil "case \"$1\" in~%submit) n=$(cat \"$MELTYPE_START_LOG_DIR/attempts\"); echo $((n+1)) > \"$MELTYPE_START_LOG_DIR/attempts\" ;;~%esac"))
  (let ((failed nil))
    (handler-case (run-script work "start-input-method.sh" "/tmp/Meltype.app")
      (uiop:subprocess-error () (setf failed t)))
    (assert failed))
  (assert (= 3 (parse-integer (uiop:read-file-string (merge-pathnames "attempts" work))))))

(defun test-failed-start-does-not-select (work)
  (test-update work)
  (write-file (merge-pathnames "calls" work) "")
  (fake-tool work "start-input-method.sh" "exit 1")
  (let ((failed nil))
    (handler-case (run-script work "update.sh" nil)
      (uiop:subprocess-error () (setf failed t)))
    (assert failed))
  (let ((lines (uiop:read-file-lines (merge-pathnames "calls" work))))
    (assert (not (member "select" lines :test #'equal)))
    (assert (not (member "app:--register-input-source" lines :test #'equal)))))

(defun replace-text (text old new)
  (with-output-to-string (out)
    (loop with start = 0
          for position = (search old text :start2 start)
          do (write-string text out :start start :end position)
          while position
          do (write-string new out)
             (setf start (+ position (length old))))))

(defun test-distributed-install (work)
  (let ((source (uiop:read-file-string (merge-pathnames "mac/install.sh" *root*))))
    (write-file (merge-pathnames "install.sh" work)
                (replace-text
                 (replace-text source "$HOME/Library/Input Methods"
                               (namestring (merge-pathnames "installed" work)))
                 "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
                 "lsregister")))
  (fake-tool work "Meltype.app/Contents/MacOS/Meltype"
             "echo \"app:$*\" >> \"$MELTYPE_TEST_LOG\"")
  (fake-tool work "start-input-method.sh"
             "echo started >> \"$MELTYPE_TEST_LOG\"")
  (write-file (merge-pathnames "install-app.sh" work)
              (uiop:read-file-string (merge-pathnames "mac/install-app.sh" *root*)))
  (fake-tool work "ditto" "cp -R \"$1\" \"$2\"")
  (fake-tool work "codesign" "exit 0")
  (write-file (merge-pathnames "select-input-source.swift" work) "// fake selector")
  (fake-tool work "swift" "echo selected >> \"$MELTYPE_TEST_LOG\"")
  (dolist (name '("pkill" "killall" "xattr" "lsregister"))
    (fake-tool work name "exit 0"))
  (fake-tool work "open" "echo UNEXPECTED_OPEN >> \"$MELTYPE_TEST_LOG\"; exit 1")
  (run-script work "install.sh" nil)
  (assert (equal '("app:--register-input-source" "started" "selected")
                 (uiop:read-file-lines (merge-pathnames "calls" work)))))

(defun test-incomplete-package (work)
  (test-distributed-install work)
  (write-file (merge-pathnames "calls" work) "")
  (delete-file (merge-pathnames "start-input-method.sh" work))
  (let ((failed nil))
    (handler-case (run-script work "install.sh" nil)
      (uiop:subprocess-error () (setf failed t)))
    (assert failed))
  (assert (null (uiop:read-file-lines (merge-pathnames "calls" work)))))

(defun test-install-rollback (work)
  (write-file (merge-pathnames "install-app.sh" work)
              (uiop:read-file-string (merge-pathnames "mac/install-app.sh" *root*)))
  (write-file (merge-pathnames "source.app/version" work) "new")
  (write-file (merge-pathnames "installed.app/version" work) "previous")
  (fake-tool work "pkill" "echo stopped >> \"$MELTYPE_TEST_LOG\"")
  (dolist (failure '("copy" "signature" "replace"))
    (write-file (merge-pathnames "calls" work) "")
    (fake-tool work "ditto" (if (equal failure "copy") "exit 23" "cp -R \"$1\" \"$2\""))
    (fake-tool work "codesign" (if (equal failure "signature") "exit 23" "exit 0"))
    (fake-tool work "mv"
               (if (equal failure "replace")
                   "if [[ \"$1\" == */Meltype.app && \"$2\" == */installed.app ]]; then exit 23; fi; exec /bin/mv \"$@\""
                   "exec /bin/mv \"$@\""))
    (let ((status nil))
      (handler-case
          (run-script work "install-app.sh"
                      (list (namestring (merge-pathnames "source.app" work))
                            (namestring (merge-pathnames "installed.app" work))))
        (uiop:subprocess-error (condition)
          (setf status (uiop:subprocess-error-code condition))))
      (assert (eql 23 status)))
    (assert (equal "previous" (uiop:read-file-string (merge-pathnames "installed.app/version" work))))
    (unless (equal failure "replace")
      (assert (null (uiop:read-file-lines (merge-pathnames "calls" work)))))))

(defun test-partial-install-rollback (work)
  (test-install-rollback work)
  (fake-tool work "mktemp"
             "mkdir -p \"$MELTYPE_START_LOG_DIR/staging\"; echo \"$MELTYPE_START_LOG_DIR/staging\"")
  (dolist (restore-fails '(nil t))
    (fake-tool work "mv"
               (concatenate 'string
                            "if [[ \"$1\" == */Meltype.app && \"$2\" == */installed.app ]]; then mkdir -p \"$2\"; echo incomplete > \"$2/version\"; exit 23; fi; "
                            (if restore-fails
                                "if [[ \"$1\" == */previous.app && \"$2\" == */installed.app ]]; then exit 27; fi; "
                                "")
                            "exec /bin/mv \"$@\""))
    (let ((status nil))
      (handler-case
          (run-script work "install-app.sh"
                      (list (namestring (merge-pathnames "source.app" work))
                            (namestring (merge-pathnames "installed.app" work))))
        (uiop:subprocess-error (condition)
          (setf status (uiop:subprocess-error-code condition))))
      (assert (eql 23 status)))
    (if restore-fails
        (progn
          (assert (equal "previous" (uiop:read-file-string (merge-pathnames "staging/previous.app/version" work))))
          (assert (equal (format nil "incomplete~%") (uiop:read-file-string (merge-pathnames "staging/failed.app/version" work))))
          (assert (not (probe-file (merge-pathnames "installed.app" work)))))
        (progn
          (assert (equal "previous" (uiop:read-file-string (merge-pathnames "installed.app/version" work))))
          (assert (not (probe-file (merge-pathnames "staging" work))))))))

(defun test-cross-volume-install-rejected (work)
  (test-install-rollback work)
  (write-file (merge-pathnames "calls" work) "")
  (fake-tool work "stat"
             "if [[ \"$3\" == */.meltype-install.* ]]; then echo 1; else echo 2; fi")
  (let ((failed nil))
    (handler-case
        (run-script work "install-app.sh"
                    (list (namestring (merge-pathnames "source.app" work))
                          (namestring (merge-pathnames "installed.app" work))))
      (uiop:subprocess-error () (setf failed t)))
    (assert failed))
  (assert (equal "previous" (uiop:read-file-string (merge-pathnames "installed.app/version" work))))
  (assert (null (uiop:read-file-lines (merge-pathnames "calls" work)))))

(handler-case
    (progn
      (call-with-work-directory #'test-build-only)
      (format t "PASS build-only routing~%")
      (call-with-work-directory #'test-update)
      (format t "PASS update routing~%")
      (call-with-work-directory #'test-launchd-start)
      (format t "PASS launchd startup~%")
      (call-with-work-directory #'test-failed-start-does-not-select)
      (format t "PASS failed startup keeps input source~%")
      (call-with-work-directory #'test-distributed-install)
      (format t "PASS distributed installer~%")
      (call-with-work-directory #'test-incomplete-package)
      (format t "PASS incomplete package preflight~%")
      (call-with-work-directory #'test-update-failure-recovery)
      (format t "PASS update failure recovery~%")
      (call-with-work-directory #'test-install-rollback)
      (format t "PASS install copy/signature/replacement failure rollback~%")
      (call-with-work-directory #'test-launchd-retry)
      (format t "PASS bounded startup retry and exhaustion~%")
      (call-with-work-directory #'test-partial-install-rollback)
      (format t "PASS partial replacement rollback and backup preservation~%")
      (call-with-work-directory #'test-cross-volume-install-rejected)
      (format t "PASS cross-volume switch rejected before stopping IME~%11/11 passed~%"))
  (error (condition)
    (format *error-output* "FAIL: ~A~%" condition)
    (uiop:quit 1)))
