CREATE TABLE departments (
    dept_id      NUMBER(5)     NOT NULL,
    dept_name    VARCHAR2(50)  NOT NULL,
    cost_center  VARCHAR2(10)  NOT NULL,
    active_flag  CHAR(1)       DEFAULT 'Y' NOT NULL,
    CONSTRAINT pk_departments     PRIMARY KEY (dept_id),
    CONSTRAINT uk_departments_cc  UNIQUE (cost_center),
    CONSTRAINT ck_dept_active     CHECK (active_flag IN ('Y','N'))
);

CREATE TABLE employees (
    emp_id            NUMBER(10)    NOT NULL,
    dept_id           NUMBER(5)     NOT NULL,
    first_name        VARCHAR2(50)  NOT NULL,
    last_name         VARCHAR2(50)  NOT NULL,
    email             VARCHAR2(100) NOT NULL,
    hire_date         DATE          DEFAULT SYSDATE NOT NULL,
    termination_date  DATE          NULL,
    salary            NUMBER(10,2)  NOT NULL,
    status            VARCHAR2(20)  DEFAULT 'ACTIVE' NOT NULL,
    CONSTRAINT pk_employees       PRIMARY KEY (emp_id),
    CONSTRAINT uk_employees_email UNIQUE (email),
    CONSTRAINT fk_employees_dept  FOREIGN KEY (dept_id) REFERENCES departments(dept_id),
    CONSTRAINT ck_emp_status      CHECK (status IN ('ACTIVE','LEAVE','TERMINATED','RETIRED')),
    CONSTRAINT ck_emp_salary      CHECK (salary > 0),
    CONSTRAINT ck_emp_term_date   CHECK (termination_date IS NULL OR termination_date >= hire_date)
);

CREATE TABLE salary_grades (
    grade_id    NUMBER(3)     NOT NULL,
    grade_name  VARCHAR2(20)  NOT NULL,
    min_salary  NUMBER(10,2)  NOT NULL,
    max_salary  NUMBER(10,2)  NOT NULL,
    CONSTRAINT pk_salary_grades PRIMARY KEY (grade_id),
    CONSTRAINT uk_grade_name    UNIQUE (grade_name),
    CONSTRAINT ck_grade_range   CHECK (max_salary > min_salary)
);

CREATE TABLE audit_log (
    audit_id     NUMBER(15)     NOT NULL,
    action_date  TIMESTAMP      DEFAULT SYSTIMESTAMP NOT NULL,
    action_by    VARCHAR2(30)   DEFAULT USER NOT NULL,
    action_type  VARCHAR2(20)   NOT NULL,
    object_name  VARCHAR2(50)   NULL,
    details      VARCHAR2(4000) NULL,
    CONSTRAINT pk_audit_log    PRIMARY KEY (audit_id),
    CONSTRAINT ck_audit_action CHECK (action_type IN ('INSERT','UPDATE','DELETE','LOGIN','EXEC'))
);
